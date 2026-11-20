#!/usr/bin/env bash
# Runs ContosoInvoicing (a foreign consumer outside demo/) against a Noelia
# release candidate and PROVES that it was the candidate that ran. Design and
# reasons: docs/adr/0005-contosoinvoicing-prueft-den-kandidaten.md (Q-0067).
#
#   contoso-candidate-check.sh --contoso <dir> --noelia <checkout> \
#       --version <X.Y.Z-gate.stamp> --feed <dir> [--keep]
#
# The feed must already hold the candidate packed under exactly that version
# (see the noelia-release skill). The version MUST carry a prerelease suffix:
# a bare 7.1.1 would also match what nuget.org or a warm cache already hold,
# and then "restored 7.1.1" proves nothing.
#
# Everything runs on a throw-away COPY of ContosoInvoicing, with one private
# package folder (NUGET_PACKAGES) for restore, build, the CLI tool and the
# service. The original is never written.
#
# NU1900 note: with NuGetAudit and an offline or blocked vulnerability feed,
# restore can emit NU1900, which -warnaserror turns into a failure. That is a
# possible false alarm of the environment, not a verdict on the candidate. The
# audit is deliberately NOT switched off here; rerun with network access.
#
# Not here (tracked separately): .env handling (#55), dropping the cache purge
# in the skill (#63).
#
# The Noelia checkout is only read (git rev-parse, packing is the caller's job).
# Source this file to use the functions (the tests do); main runs only when the
# file is executed.

set -euo pipefail

CCC_PACKAGE_RE='^[0-9]+\.[0-9]+\.[0-9]+-[0-9A-Za-z.-]+$'

ccc_log()  { printf '%s\n' "$*"; }
ccc_fail() { printf 'FAIL: %s\n' "$*" >&2; return 1; }
# A failing check must stop the script even inside "if ! ..." contexts, so the
# top-level callers use `|| exit 1`; the functions themselves only return.

# ---------------------------------------------------------------- validation

ccc_validate_version() {
  local v="${1:-}"
  if [[ ! "$v" =~ $CCC_PACKAGE_RE ]]; then
    ccc_fail "version '$v' is not X.Y.Z-<prerelease>; a candidate needs a unique prerelease suffix (e.g. 7.1.1-gate.20261120120000)"
    return 64
  fi
}

# Paths end up in sed and in XML: refuse what would break either.
ccc_validate_path_safe() {
  local label="$1" p="${2:-}"
  case "$p" in
    '') ccc_fail "$label is empty"; return 64 ;;
    *'&'*|*'<'*|*'>'*|*'"'*|*"'"*|*'|'*|*'\'*|*'
'*) ccc_fail "$label contains a character that is unsafe in XML or sed: $p"; return 65 ;;
  esac
}

# At least 128 bit of randomness as hex (32 chars), never empty.
ccc_validate_secret() {
  local s="${1:-}"
  if [[ ! "$s" =~ ^[0-9A-Fa-f]{32,}$ ]]; then
    ccc_fail "operator secret is missing, empty or shorter than 128 bit of hex"
    return 64
  fi
}

ccc_generate_secret() { openssl rand -hex 32; }

# ------------------------------------------------------------------- the copy

ccc_copy_contoso() {
  local src="$1" dst="$2"
  rsync -a \
    --exclude '.git/' \
    --exclude '.env' \
    --exclude '.env.*' \
    --exclude 'bin/' \
    --exclude 'obj/' \
    --exclude 'logs/' \
    "$src/" "$dst/"
  # The copy must not inherit configuration that could redirect restore.
  rm -f -- "$dst/nuget.config" "$dst/NuGet.config" "$dst/NuGet.Config" "$dst/NuGet.Local.Config"
}

# Every Noelia.* PackageReference gets the candidate version.
ccc_rewrite_pins() {
  local csproj="$1" version="$2" tmp
  tmp="$(mktemp)"
  sed -E "s|(<PackageReference[[:space:]]+Include=\"Noelia\\.[^\"]+\"[[:space:]]+Version=\")[^\"]*(\")|\\1${version}\\2|g" \
    "$csproj" > "$tmp"
  cat "$tmp" > "$csproj"
  rm -f -- "$tmp"
}

# Outside the copy, <clear/>, Noelia.* only from the candidate feed.
ccc_write_config() {
  local out="$1" feed="$2"
  cat > "$out" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="candidate" value="$feed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="candidate">
      <package pattern="Noelia.*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
EOF
}

# ------------------------------------------------------------------- the proof

# Every Noelia.* library in the resolved graph must be exactly the candidate.
ccc_check_assets() {
  local assets="$1" version="$2" found=0 lib
  [[ -f "$assets" ]] || { ccc_fail "no project.assets.json at $assets; nothing was restored"; return 1; }
  while IFS= read -r lib; do
    found=1
    if [[ "${lib##*/}" != "$version" ]]; then
      ccc_fail "resolved $lib, expected version $version"
      return 1
    fi
  done < <(jq -r '.libraries | keys[] | select(startswith("Noelia."))' "$assets")
  if [[ "$found" != 1 ]]; then
    ccc_fail "no Noelia package resolved; the candidate was not tested"
    return 1
  fi
}

ccc_nuspec_commit() {
  sed -nE 's/.*<repository[^>]*[[:space:]]commit="([0-9a-fA-F]+)".*/\1/p' "$1" | head -n1
}

# For EVERY restored Noelia.* package: its nuspec must name the checkout's HEAD.
# A missing commit is a FAIL, never a skip: a package without it cannot be tied
# to the checkout, which is the very thing this check exists for.
ccc_check_commits() {
  local pkgs="$1" version="$2" expected="$3" nuspec commit n=0
  while IFS= read -r nuspec; do
    n=$((n + 1))
    commit="$(ccc_nuspec_commit "$nuspec")"
    if [[ -z "$commit" ]]; then
      ccc_fail "$nuspec has no repository commit; the build cannot be tied to the checkout"
      return 1
    fi
    if [[ "$commit" != "$expected" ]]; then
      ccc_fail "$nuspec was built from $commit, the checkout is at $expected"
      return 1
    fi
  done < <(find "$pkgs" -path "*/noelia.*/$version/*.nuspec" -type f | sort)
  if [[ "$n" == 0 ]]; then
    ccc_fail "no restored Noelia.* nuspec for $version below $pkgs"
    return 1
  fi
}

# "7.1.1-gate.x+abc123" -> version and sha, separated.
ccc_split_cli_version() {
  local out="$1"
  CCC_CLI_VERSION="${out%%+*}"
  CCC_CLI_SHA=""
  [[ "$out" == *+* ]] && CCC_CLI_SHA="${out#*+}"
  CCC_CLI_VERSION="${CCC_CLI_VERSION//[$'\r\n ']/}"
  CCC_CLI_SHA="${CCC_CLI_SHA//[$'\r\n ']/}"
}

ccc_check_cli_version() {
  local out="$1" version="$2" head="$3"
  ccc_split_cli_version "$out"
  if [[ "$CCC_CLI_VERSION" != "$version" ]]; then
    ccc_fail "noelia --version says $CCC_CLI_VERSION, expected $version"; return 1
  fi
  if [[ -z "$CCC_CLI_SHA" || "$head" != "$CCC_CLI_SHA"* ]]; then
    ccc_fail "noelia --version sha '$CCC_CLI_SHA' does not match checkout $head"; return 1
  fi
}

# The folder that was in force BEFORE NUGET_PACKAGES is exported for the run.
ccc_global_packages_folder() {
  if [[ -n "${NUGET_PACKAGES:-}" ]]; then printf '%s\n' "$NUGET_PACKAGES"; return; fi
  local listed=""
  if command -v dotnet >/dev/null 2>&1; then
    listed="$(dotnet nuget locals global-packages --list 2>/dev/null | sed -nE 's/^global-packages:[[:space:]]*//p' | head -n1)" || true
  fi
  printf '%s\n' "${listed:-$HOME/.nuget/packages}"
}

# Gate condition: the run must not have leaked a candidate into the real cache.
ccc_check_global_clean() {
  local folder="$1" version="$2" leak
  leak="$(find "$folder" -maxdepth 2 -path "$folder/noelia.*/$version" 2>/dev/null | head -n1 || true)"
  if [[ -n "$leak" ]]; then
    ccc_fail "candidate leaked into the global package folder: $leak"
    return 1
  fi
}

# ------------------------------------------------------------------- lifecycle

CCC_TEMP=""; CCC_SERVICE_PID=""; CCC_KEEP=0

ccc_stop_service() {
  [[ -n "$CCC_SERVICE_PID" ]] || return 0
  if kill -0 "$CCC_SERVICE_PID" 2>/dev/null; then
    kill -TERM "$CCC_SERVICE_PID" 2>/dev/null || true
    local i
    for i in 1 2 3 4 5 6 7 8 9 10; do
      kill -0 "$CCC_SERVICE_PID" 2>/dev/null || break
      sleep 0.5
    done
    kill -KILL "$CCC_SERVICE_PID" 2>/dev/null || true
  fi
  wait "$CCC_SERVICE_PID" 2>/dev/null || true
  CCC_SERVICE_PID=""
}

# Service first, directory second: a running service holds files open.
ccc_cleanup() {
  local rc=$?
  trap - EXIT INT TERM
  ccc_stop_service
  if [[ -n "$CCC_TEMP" && -d "$CCC_TEMP" ]]; then
    if [[ "$CCC_KEEP" == 1 ]]; then
      ccc_log "kept: $CCC_TEMP (service logs there are redacted)"
    else
      rm -rf -- "$CCC_TEMP"
    fi
  fi
  return $rc
}

ccc_free_port() {
  local p
  for p in $(seq 5311 5399); do
    if ! (exec 3<>"/dev/tcp/127.0.0.1/$p") 2>/dev/null; then printf '%s\n' "$p"; return; fi
  done
  ccc_fail "no free port in 5311-5399"; return 1
}

ccc_redact() { sed -e "s/${CCC_SECRET:-__none__}/<redacted>/g"; }

# Waits for /health with a deadline; aborts early when the process dies.
ccc_wait_health() {
  local pid="$1" url="$2" log="$3" deadline=$((SECONDS + 60))
  while (( SECONDS < deadline )); do
    if ! kill -0 "$pid" 2>/dev/null; then
      ccc_fail "service exited before /health answered; log tail:"
      tail -n 20 "$log" | ccc_redact >&2
      return 1
    fi
    if curl -fsS -o /dev/null --max-time 2 "$url" 2>/dev/null; then return 0; fi
    sleep 1
  done
  ccc_fail "no /health within 60 s; log tail:"
  tail -n 20 "$log" | ccc_redact >&2
  return 1
}

ccc_usage() {
  echo "Usage: $0 --contoso <dir> --noelia <checkout> --version <X.Y.Z-prerelease> --feed <dir> [--keep]" >&2
  exit 64
}

ccc_main() {
  local contoso="" noelia="" version="" feed_in="" arg
  while [[ $# -gt 0 ]]; do
    arg="$1"; shift
    case "$arg" in
      --contoso) contoso="${1:-}"; shift || true ;;
      --noelia)  noelia="${1:-}";  shift || true ;;
      --version) version="${1:-}"; shift || true ;;
      --feed)    feed_in="${1:-}"; shift || true ;;
      --keep)    CCC_KEEP=1 ;;
      *) echo "Unknown argument: $arg" >&2; ccc_usage ;;
    esac
  done

  if [[ -z "$contoso" ]]; then
    echo "FAIL: --contoso <dir> is required (no default; e.g. ~/Projects/ContosoInvoicing)." >&2
    echo "ContosoInvoicing here would be: $HOME/Projects/ContosoInvoicing @ $(git -C "$HOME/Projects/ContosoInvoicing" rev-parse HEAD 2>/dev/null || echo 'unknown')" >&2
    exit 64
  fi
  [[ -n "$noelia" && -n "$version" && -n "$feed_in" ]] || ccc_usage
  ccc_validate_version "$version" || exit $?
  ccc_validate_path_safe contoso "$contoso" || exit $?
  ccc_validate_path_safe noelia "$noelia" || exit $?
  ccc_validate_path_safe feed "$feed_in" || exit $?
  [[ -f "$contoso/ContosoInvoicing.csproj" ]] || { echo "FAIL: no ContosoInvoicing.csproj in $contoso" >&2; exit 66; }
  [[ -d "$feed_in" ]] || { echo "FAIL: feed does not exist: $feed_in" >&2; exit 66; }

  local feed head dirty global
  feed="$(cd "$feed_in" && pwd)"
  head="$(git -C "$noelia" rev-parse HEAD)"
  dirty=""; [[ -n "$(git -C "$noelia" status --porcelain)" ]] && dirty=" (DIRTY tree: the packed bits may not equal this commit)"
  ccc_log "candidate $version at ${head}${dirty}"
  global="$(ccc_global_packages_folder)"   # before the export below

  CCC_TEMP="$(mktemp -d "${TMPDIR:-/tmp}/contoso-candidate.XXXXXX")"
  trap ccc_cleanup EXIT INT TERM
  export NUGET_PACKAGES="$CCC_TEMP/pkgs"
  mkdir -p "$NUGET_PACKAGES"

  local work="$CCC_TEMP/work" config="$CCC_TEMP/NuGet.Config" log="$CCC_TEMP/service.log"
  mkdir -p "$work"
  ccc_copy_contoso "$contoso" "$work"
  ccc_rewrite_pins "$work/ContosoInvoicing.csproj" "$version"
  ccc_write_config "$config" "$feed"

  dotnet restore "$work/ContosoInvoicing.csproj" --configfile "$config" --force --no-cache
  ccc_check_assets "$work/obj/project.assets.json" "$version" || exit 1
  ccc_check_commits "$NUGET_PACKAGES" "$version" "$head" || exit 1

  dotnet build "$work/ContosoInvoicing.csproj" -c Release --no-restore -warnaserror

  # The candidate's own tool, from the same private folder, without a TTY.
  [[ -t 0 ]] && ccc_log "note: stdin is a TTY; the tool is started with stdin closed"
  local cli_out
  cli_out="$(dotnet dnx Noelia.Cli --version "$version" --source "$feed" --configfile "$config" --yes -- --version </dev/null)"
  [[ -d "$NUGET_PACKAGES/noelia.cli/$version" ]] || { echo "FAIL: noelia.cli/$version not in the private folder" >&2; exit 1; }
  ccc_check_cli_version "$cli_out" "$version" "$head" || exit 1
  local noelia_cli=(dotnet dnx Noelia.Cli --version "$version" --source "$feed" --configfile "$config" --yes --)

  # Throw-away secret: never printed, never set -x.
  CCC_SECRET="$(ccc_generate_secret)"
  ccc_validate_secret "$CCC_SECRET" || exit 1

  local analyze_rc=0
  "${noelia_cli[@]}" analyze "$work" </dev/null || analyze_rc=$?
  [[ "$analyze_rc" == 2 ]] && { echo "FAIL: noelia analyze <dir> exited 2 (tool error)" >&2; exit 1; }
  ccc_log "analyze <dir> exit $analyze_rc (1 = findings, reported not fatal)"

  local port dll
  port="$(ccc_free_port)"
  dll="$(find "$work/bin/Release" -name ContosoInvoicing.dll -type f | head -n1)"
  [[ -n "$dll" ]] || { echo "FAIL: built ContosoInvoicing.dll not found" >&2; exit 1; }
  ASPNETCORE_ENVIRONMENT=Production ASPNETCORE_URLS="http://127.0.0.1:$port" Operator__Secret="$CCC_SECRET" \
    dotnet "$dll" >"$log" 2>&1 </dev/null &
  CCC_SERVICE_PID=$!
  ccc_wait_health "$CCC_SERVICE_PID" "http://127.0.0.1:$port/health" "$log" || exit 1
  kill -0 "$CCC_SERVICE_PID" 2>/dev/null || { echo "FAIL: service pid is gone after health OK (foreign process on the port?)" >&2; exit 1; }

  local report
  report="$(printf 'header = "X-Noelia-Operator: %s"\n' "$CCC_SECRET" | curl -fsS -K - "http://127.0.0.1:$port/noelia/report.json")"
  [[ "$(jq -r '.service' <<<"$report")" == invoicing ]] || { echo "FAIL: report.json service != invoicing" >&2; exit 1; }
  [[ "$(jq -r '.environment' <<<"$report")" == Production ]] || { echo "FAIL: report.json environment != Production" >&2; exit 1; }

  analyze_rc=0
  local live_out
  live_out="$("${noelia_cli[@]}" analyze --url "http://127.0.0.1:$port" --operator-secret "$CCC_SECRET" </dev/null 2>&1)" || analyze_rc=$?
  printf '%s\n' "$live_out" | ccc_redact
  [[ "$analyze_rc" == 2 ]] && { echo "FAIL: noelia analyze --url exited 2 (tool error, e.g. unknown report schema)" >&2; exit 1; }

  ccc_stop_service
  ccc_check_global_clean "$global" "$version" || exit 1
  ccc_log "ContosoInvoicing ran against Noelia $version @ $head: pin, source, tool, secret, commit verified."
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
  ccc_main "$@"
fi
