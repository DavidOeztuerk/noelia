#!/usr/bin/env bash
# Offline tests for contoso-candidate-check.sh: input validation, pin rewrite,
# generated config, assets/commit proofs, copy exclusions, cleanup. No network,
# no packages, no dotnet. The full run against a real candidate is the evidence
# of the next release, not of this file.
#
#   bash demo/eng/test_contoso_candidate_check.sh
set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPT="$HERE/contoso-candidate-check.sh"
[[ -f "$SCRIPT" ]] || { echo "missing $SCRIPT" >&2; exit 1; }
# shellcheck source=/dev/null
source "$SCRIPT"
set +e   # the sourced file turned errexit on; tests assert on exit codes themselves

PASS=0; FAILED=0
T="$(mktemp -d "${TMPDIR:-/tmp}/ccc-test.XXXXXX")"
trap 'rm -rf -- "$T"' EXIT

ok()   { PASS=$((PASS + 1)); echo "ok   - $1"; }
bad()  { FAILED=$((FAILED + 1)); echo "FAIL - $1"; }
# expect_fail <name> <cmd...>: must return non-zero (stderr swallowed)
expect_fail() { local n="$1"; shift; if "$@" >/dev/null 2>&1; then bad "$n"; else ok "$n"; fi; }
expect_ok()   { local n="$1"; shift; if "$@" >/dev/null 2>&1; then ok "$n"; else bad "$n"; fi; }

V="7.1.1-gate.20261120120000"
HEAD_SHA="0123456789abcdef0123456789abcdef01234567"

# --- version
expect_ok   "version with prerelease accepted" ccc_validate_version "$V"
expect_fail "bare version rejected (no prerelease)" ccc_validate_version "7.1.1"
expect_fail "garbage version rejected" ccc_validate_version "7.1; rm -rf"
expect_fail "empty version rejected" ccc_validate_version ""

# --- paths
expect_ok   "plain path accepted" ccc_validate_path_safe feed "/tmp/a b/feed"
expect_fail "ampersand path rejected" ccc_validate_path_safe feed "/tmp/a&b"
expect_fail "angle bracket path rejected" ccc_validate_path_safe feed "/tmp/<x>"
expect_fail "quote path rejected" ccc_validate_path_safe feed '/tmp/a"b'
expect_fail "pipe path rejected (sed delimiter)" ccc_validate_path_safe feed "/tmp/a|b"
expect_fail "empty path rejected" ccc_validate_path_safe feed ""

# --- secret
expect_fail "empty secret rejected" ccc_validate_secret ""
expect_fail "short secret rejected" ccc_validate_secret "abcd"
expect_fail "non-hex secret rejected" ccc_validate_secret "$(printf 'z%.0s' $(seq 40))"
expect_ok   "generated secret accepted" ccc_validate_secret "$(ccc_generate_secret)"

# --- missing --contoso is loud, mentions the path, exits 64
out="$("$SCRIPT" --noelia /x --version "$V" --feed /x 2>&1)"; rc=$?
[[ $rc == 64 && "$out" == *"--contoso"* ]] && ok "missing --contoso aborts loudly (64)" || bad "missing --contoso aborts loudly (rc=$rc)"
out="$("$SCRIPT" --contoso /x --noelia /x --version 7.1.1 --feed /x 2>&1)"; rc=$?
[[ $rc == 64 ]] && ok "script rejects bare version (64)" || bad "script rejects bare version (rc=$rc)"

# --- pin rewrite
cat > "$T/fx.csproj" <<'EOF'
<Project Sdk="Microsoft.NET.Sdk.Web">
  <ItemGroup>
    <PackageReference Include="Noelia.Infrastructure" Version="6.4.0" />
    <PackageReference Include="Noelia.Dashboard" Version="6.4.0" />
    <PackageReference   Include="Noelia.InMemory"   Version="6.3.0" />
    <PackageReference Include="Serilog" Version="4.0.0" />
  </ItemGroup>
</Project>
EOF
ccc_rewrite_pins "$T/fx.csproj" "$V"
n_new="$(grep -c "Include=\"Noelia\.[^\"]*\" *Version=\"$V\"" "$T/fx.csproj")"
n_noelia="$(grep -c 'Include="Noelia\.' "$T/fx.csproj")"
[[ "$n_noelia" == 3 && "$n_new" == 3 ]] && ok "all Noelia.* pins rewritten" || bad "pins rewritten ($n_new/$n_noelia)"
grep -q 'Include="Serilog" Version="4.0.0"' "$T/fx.csproj" && ok "foreign pin untouched" || bad "foreign pin untouched"

# --- generated config
ccc_write_config "$T/NuGet.Config" "/tmp/feed dir"
c="$(cat "$T/NuGet.Config")"
[[ "$c" == *"<clear />"* ]] && ok "config clears sources" || bad "config clears sources"
[[ "$c" == *'key="candidate" value="/tmp/feed dir"'* ]] && ok "config names candidate feed" || bad "config names candidate feed"
# Noelia.* appears only under the candidate source
sec_candidate="$(sed -n '/<packageSource key="candidate">/,/<\/packageSource>/p' "$T/NuGet.Config")"
sec_nuget="$(sed -n '/<packageSource key="nuget.org">/,/<\/packageSource>/p' "$T/NuGet.Config")"
[[ "$sec_candidate" == *'pattern="Noelia.*"'* && "$sec_nuget" != *Noelia* ]] && ok "Noelia.* mapped to candidate only" || bad "Noelia.* mapped to candidate only"

# --- assets
good="$T/good.assets.json"; badj="$T/bad.assets.json"; none="$T/none.assets.json"
printf '{"libraries":{"Noelia.Core/%s":{},"Noelia.Infrastructure/%s":{},"Serilog/4.0.0":{}}}' "$V" "$V" > "$good"
printf '{"libraries":{"Noelia.Core/%s":{},"Noelia.Infrastructure/6.4.0":{}}}' "$V" > "$badj"
printf '{"libraries":{"Serilog/4.0.0":{}}}' > "$none"
expect_ok   "assets: all candidate passes" ccc_check_assets "$good" "$V"
expect_fail "assets: deviating version aborts" ccc_check_assets "$badj" "$V"
expect_fail "assets: no Noelia package aborts" ccc_check_assets "$none" "$V"
expect_fail "assets: missing file aborts" ccc_check_assets "$T/nope.json" "$V"

# --- commits
mk_pkg() { # <pkgs> <name> <repository element or empty>
  mkdir -p "$1/$2/$V"
  printf '<package><metadata><id>%s</id>%s</metadata></package>' "$2" "$3" > "$1/$2/$V/$2.nuspec"
}
P1="$T/p1"; mk_pkg "$P1" noelia.core "<repository type=\"git\" url=\"u\" commit=\"$HEAD_SHA\" />"
mk_pkg "$P1" noelia.infrastructure "<repository type=\"git\" url=\"u\" commit=\"$HEAD_SHA\" />"
expect_ok   "commits: all match" ccc_check_commits "$P1" "$V" "$HEAD_SHA"
P2="$T/p2"; mk_pkg "$P2" noelia.core "<repository type=\"git\" url=\"u\" commit=\"$HEAD_SHA\" />"
mk_pkg "$P2" noelia.infrastructure "<repository type=\"git\" url=\"u\" commit=\"ffffffffffffffffffffffffffffffffffffffff\" />"
expect_fail "commits: one package from another build aborts" ccc_check_commits "$P2" "$V" "$HEAD_SHA"
P3="$T/p3"; mk_pkg "$P3" noelia.core "<repository type=\"git\" url=\"u\" commit=\"$HEAD_SHA\" />"
mk_pkg "$P3" noelia.infrastructure ""
expect_fail "commits: missing repository@commit is FAIL, not skip" ccc_check_commits "$P3" "$V" "$HEAD_SHA"
expect_fail "commits: no nuspec at all aborts" ccc_check_commits "$T/empty" "$V" "$HEAD_SHA"

# --- CLI version split
expect_ok   "cli: version+sha match" ccc_check_cli_version "$V+${HEAD_SHA:0:9}" "$V" "$HEAD_SHA"
expect_fail "cli: sha mismatch aborts" ccc_check_cli_version "$V+deadbeef" "$V" "$HEAD_SHA"
expect_fail "cli: version mismatch aborts" ccc_check_cli_version "6.4.0+${HEAD_SHA:0:9}" "$V" "$HEAD_SHA"
expect_fail "cli: missing sha aborts" ccc_check_cli_version "$V" "$V" "$HEAD_SHA"

# --- global folder
[[ "$(NUGET_PACKAGES=/some/dir ccc_global_packages_folder)" == /some/dir ]] && ok "global folder honours NUGET_PACKAGES" || bad "global folder honours NUGET_PACKAGES"
G="$T/global"; mkdir -p "$G/serilog/4.0.0"
expect_ok   "global clean passes" ccc_check_global_clean "$G" "$V"
mkdir -p "$G/noelia.core/$V"
expect_fail "leaked candidate in global folder aborts" ccc_check_global_clean "$G" "$V"

# --- copy exclusions
S="$T/src"; D="$T/dst"; mkdir -p "$S/.git" "$S/bin" "$S/obj" "$S/logs" "$D"
for f in .git/HEAD .env .env.local bin/x obj/x logs/x nuget.config NuGet.Local.Config Program.cs; do : > "$S/$f"; done
ccc_copy_contoso "$S" "$D"
leftover="$(cd "$D" && ls -A)"
[[ "$leftover" == "Program.cs" ]] && ok "copy keeps only sources (no .git/.env*/bin/obj/logs/nuget configs)" || bad "copy leftovers: $leftover"

# --- cleanup trap: service stopped, temp removed (and kept with --keep)
CCC_TEMP="$T/cl"; mkdir -p "$CCC_TEMP"; CCC_KEEP=0
sleep 30 & CCC_SERVICE_PID=$!; svc=$CCC_SERVICE_PID
( ccc_cleanup ) >/dev/null 2>&1
if [[ ! -d "$T/cl" ]] && ! kill -0 "$svc" 2>/dev/null; then ok "cleanup stops service and removes temp"; else bad "cleanup stops service and removes temp"; kill "$svc" 2>/dev/null; fi
CCC_TEMP="$T/cl2"; mkdir -p "$CCC_TEMP"; CCC_KEEP=1; CCC_SERVICE_PID=""
( ccc_cleanup ) >/dev/null 2>&1
[[ -d "$T/cl2" ]] && ok "cleanup keeps temp with --keep" || bad "cleanup keeps temp with --keep"
grep -qE "trap ccc_cleanup EXIT INT TERM" "$SCRIPT" && ok "cleanup trap installed on EXIT INT TERM" || bad "cleanup trap installed"

echo "$PASS passed, $FAILED failed"
[[ $FAILED == 0 ]]
